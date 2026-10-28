using RPM.Core.Playback;

namespace RPM.Desktop.Presentation.RPaMp.Models;

/// <summary>
/// The Now playing card's shuffle key. One press puts what is still to come in another order; the next press
/// puts it back in the order it had (see <see cref="QueueShuffle"/> for which rows move and why).
///
/// <para>This is not <see cref="Shuffle"/>. That one is the mini player's toggle, which leaves the queue as it
/// is and picks the next song at random. This one changes the queue itself, so the card shows what will play
/// next, in the order it will play.</para>
///
/// <para>A radio's rows are held where they are (<see cref="PlaylistTrack.RadioQueued"/>): the mixtape that
/// carries on after an album stays after the album, and only the album's own songs change places.</para>
///
/// <para>The order put aside lives only as long as this queue: a new queue drops it, and it is not saved with
/// the session. The shuffled order itself is saved, as any reorder is.</para>
/// </summary>
public sealed partial class RPaMpModel
{
    /// <summary>The queue as it stood before the shuffle, or null when it is not shuffled.</summary>
    private List<PlaylistTrack>? _unshuffled;

    /// <summary>True while <see cref="Arrange"/> is moving rows, so each move does not settle up on its
    /// own.</summary>
    private bool _arranging;

    /// <summary>True while a shuffle is moving rows. A list that mirrors the queue can wait for the last of the
    /// moves rather than following each one.</summary>
    public bool IsArrangingQueue => _arranging;

    /// <summary>Whether the queue is in a shuffled order that the shuffle key can put back.</summary>
    public bool IsQueueShuffled => _unshuffled is not null;

    /// <summary>Whether the shuffle key can do anything: put a shuffled queue back, or shuffle a queue with
    /// two or more songs still to come.</summary>
    public bool CanShuffleQueue => IsQueueShuffled || QueueShuffle.CanShuffle(Playlist, _currentIndex, IsRadioRow);

    /// <summary>A row a radio queued: the mixtape that carries on after the list, or a song radio. The shuffle
    /// holds these where they are. The mixtape is what comes after the album, not part of it, and a radio picks
    /// each song to follow the one before.</summary>
    private static bool IsRadioRow(PlaylistTrack row) => row.RadioQueued;

    /// <summary>The shuffle key: shuffles what is still to come, or puts a shuffled queue back.</summary>
    public void ToggleQueueShuffle()
    {
        if (_unshuffled is { } before)
        {
            _unshuffled = null;
            Arrange(QueueShuffle.Restored(Playlist, _currentIndex, before, IsRadioRow));
        }
        else
        {
            if (!QueueShuffle.CanShuffle(Playlist, _currentIndex, IsRadioRow))
                return;
            var kept = Playlist.ToList();
            Arrange(QueueShuffle.Shuffled(Playlist, _currentIndex, _rng, IsRadioRow));
            _unshuffled = kept;
        }

        OnChanged(nameof(IsQueueShuffled));
    }

    private void ForgetUnshuffled()
    {
        if (_unshuffled is null)
            return;
        _unshuffled = null;
        OnChanged(nameof(IsQueueShuffled));
    }

    /// <summary>
    /// Puts the queue in <paramref name="order"/>, the same rows in another order.
    ///
    /// <para>The rows are moved, not rebuilt, as the DJ's running order is applied (see ApplyDjOrder): the
    /// playing row stays the playing row, and a list showing the queue keeps its rows. What a hand drag sets
    /// off (the chain dropped, the gutter renumbered, the queue saved) is done once at the end, not once for
    /// each row that moved.</para>
    /// </summary>
    private void Arrange(IReadOnlyList<PlaylistTrack> order)
    {
        var moved = false;
        _arranging = true;
        try
        {
            for (var at = 0; at < order.Count && at < Playlist.Count; at++)
            {
                if (ReferenceEquals(Playlist[at], order[at]))
                    continue;

                var from = -1;
                for (var i = at + 1; i < Playlist.Count; i++)
                {
                    if (!ReferenceEquals(Playlist[i], order[at]))
                        continue;
                    from = i;
                    break;
                }

                if (from < 0)
                    continue;
                Playlist.Move(from, at);
                moved = true;
            }
        }
        finally
        {
            _arranging = false;
        }

        if (!moved)
            return;
        AfterReorder();
        RememberQueue();
    }
}
