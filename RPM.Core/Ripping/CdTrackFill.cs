namespace RPM.Core.Ripping;

/// <summary>
/// Which sectors of one CD track have arrived, while the track is being read and played at the same time.
///
/// <para>It decides what the drive reads next. That is normally the next sector, but a listener can jump
/// past the read. Audio drives read slowly, so waiting for the read to get there can take a minute. So a jump
/// (see <see cref="Jump"/>) moves the read to where the listener went. When the read reaches the end of the
/// track it goes back for the gaps it left (see <see cref="Next"/>).</para>
///
/// <para>A listener must not reach audio that has not arrived. The decoder would read silence there and
/// never go back for the music. So the player starts when <see cref="Enough"/> says so, and a jump lands when
/// the task from <see cref="Jump"/> completes. Calls may come from any thread.</para>
/// </summary>
/// <param name="totalSectors">The length of the whole track.</param>
/// <param name="startLeadSeconds">How much audio must have arrived before the track starts.</param>
/// <param name="jumpLeadSeconds">How much audio must have arrived at the place a jump lands on. It is shorter
/// than the start's, because a listener is waiting for it while something else is already playing.</param>
public sealed class CdTrackFill(int totalSectors, double startLeadSeconds, double jumpLeadSeconds)
{
    /// <summary>When the audio a jump wants is this close ahead of the read, the read simply carries on. Moving
    /// the drive's laser would take longer than letting it arrive.</summary>
    private const double CloseEnoughSeconds = 2;

    /// <summary>The audio in one sector, in seconds, for a caller that works in time.</summary>
    private const int SectorsPerSecond = DiscToc.FramesPerSecond;

    private readonly object _gate = new();
    private readonly TaskCompletionSource _enough = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>What has arrived: ranges that do not touch, in order, each from its first sector up to but not
    /// including its last.</summary>
    private readonly List<(int From, int To)> _filled = [];

    /// <summary>Where the listener last wanted to be. The read goes for the first gap from here.</summary>
    private int _aim;

    /// <summary>The read in progress: where it has got to, where it will stop, and the way to stop it early.</summary>
    private int _head;
    private int _runEnd;
    private CancellationTokenSource? _run;

    /// <summary>A jump waiting for its audio: it completes when everything up to the sector has arrived.</summary>
    private int _waitFrom;
    private int _waitTo;
    private TaskCompletionSource? _waiter;

    private bool _stopped;

    /// <summary>Completes when the start of the track has arrived, or all of a track shorter than that. A
    /// caller must also watch the read itself, because a read that fails never gets here.</summary>
    public Task Enough => _enough.Task;

    /// <summary>The last sector that has arrived, plus one. Anything before this may still have gaps.</summary>
    public int Furthest
    {
        get
        {
            lock (_gate)
                return _filled.Count > 0 ? _filled[^1].To : 0;
        }
    }

    /// <summary>What to read next, or null when the whole track has arrived.</summary>
    /// <param name="run">How <see cref="Jump"/> stops this read to send it somewhere else.</param>
    public (int From, int Count)? Next(CancellationTokenSource run)
    {
        lock (_gate)
        {
            var from = FirstGap(_aim) ?? FirstGap(0);
            if (from is not { } start)
                return null;

            var end = EndOfGap(start);
            _run = run;
            _head = start;
            _runEnd = end;
            return (start, end - start);
        }
    }

    /// <summary>Notes that <paramref name="count"/> sectors from <paramref name="from"/> have arrived.</summary>
    public void Arrived(int from, int count)
    {
        if (count <= 0)
            return;

        TaskCompletionSource? landed = null;
        var enough = false;
        lock (_gate)
        {
            Add(from, Math.Min(from + count, totalSectors));
            if (from + count > _head)
                _head = from + count;

            if (_waiter is not null && FirstGapBetween(_waitFrom, _waitTo) is null)
            {
                landed = _waiter;
                _waiter = null;
            }

            enough = FirstGap(0) is not { } gap || gap >= startLeadSeconds * SectorsPerSecond;
        }

        landed?.TrySetResult();
        if (enough)
            _enough.TrySetResult();
    }

    /// <summary>The progress of one read, for a reader that reports the part it has done, from 0 to 1.</summary>
    public IProgress<double> ProgressOf(int from, int count) => new ReadProgress(this, from, count);

    /// <summary>Moves the read to where the listener is going, unless it is nearly there already.</summary>
    /// <returns>A task that completes when the audio there has arrived, or when the read has stopped. It never
    /// fails. A later jump completes this one, so a caller should check that its jump is still wanted.</returns>
    public Task Jump(double seconds)
    {
        CancellationTokenSource? stop = null;
        TaskCompletionSource? replaced;
        TaskCompletionSource? waiter = null;
        lock (_gate)
        {
            var at = Math.Clamp((int)(seconds * SectorsPerSecond), 0, totalSectors);
            var to = Math.Min(at + (int)Math.Ceiling(jumpLeadSeconds * SectorsPerSecond), totalSectors);

            replaced = _waiter;
            _waiter = null;
            _aim = at;

            // The audio that follows the listener comes first, even when the place itself has arrived.
            if (!_stopped && FirstGap(at) is { } gap)
            {
                var close = gap >= _head && gap < _runEnd
                    && gap - _head <= CloseEnoughSeconds * SectorsPerSecond;
                if (!close)
                    stop = _run;
            }

            if (!_stopped && FirstGapBetween(at, to) is not null)
            {
                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiter = waiter;
                _waitFrom = at;
                _waitTo = to;
            }
        }

        // Outside the lock, because both of these can run other code on this thread.
        replaced?.TrySetResult();
        try { stop?.Cancel(); }
        catch (ObjectDisposedException) { /* that read has already ended */ }

        return waiter?.Task ?? Task.CompletedTask;
    }

    /// <summary>Says the read is over, whole or not. A jump still waiting for its audio is let go.</summary>
    public void Stop()
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            _stopped = true;
            _run = null;
            waiter = _waiter;
            _waiter = null;
        }
        waiter?.TrySetResult();
    }

    /// <summary>The first sector from <paramref name="from"/> on that has not arrived, or null.</summary>
    private int? FirstGap(int from) => FirstGapBetween(from, totalSectors);

    private int? FirstGapBetween(int from, int to)
    {
        var at = from;
        foreach (var (start, end) in _filled)
        {
            if (at < start)
                break;
            if (at < end)
                at = end;
        }
        return at < to ? at : null;
    }

    /// <summary>Where the gap that holds <paramref name="at"/> ends.</summary>
    private int EndOfGap(int at)
    {
        foreach (var (start, _) in _filled)
            if (start > at)
                return start;
        return totalSectors;
    }

    /// <summary>Adds a range to the list, joining it to any range it touches.</summary>
    private void Add(int from, int to)
    {
        var i = 0;
        while (i < _filled.Count && _filled[i].To < from)
            i++;

        while (i < _filled.Count && _filled[i].From <= to)
        {
            from = Math.Min(from, _filled[i].From);
            to = Math.Max(to, _filled[i].To);
            _filled.RemoveAt(i);
        }

        _filled.Insert(i, (from, to));
    }

    /// <summary>A reader says how far through its read it is, and this turns that into sectors.</summary>
    private sealed class ReadProgress(CdTrackFill fill, int from, int count) : IProgress<double>
    {
        public void Report(double value) =>
            fill.Arrived(from, (int)Math.Floor(Math.Clamp(value, 0, 1) * count + 1e-6));
    }
}
