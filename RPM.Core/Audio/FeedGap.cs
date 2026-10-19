namespace RPM.Core.Audio;

/// <summary>
/// One turn of the pump: the time between the sink letting go of it and it coming back with the next
/// block, and where that time went.
///
/// <para>The sink can report that the device ran dry and for how long. It cannot report what the pump
/// was doing, and "the pump was away" is only the start of the question. A slow read means the decoder
/// could not keep up. A slow changeover means the next record was set up slowly on the deadline
/// thread. A gap that no named part explains means the thread was not running at all. These three
/// causes need different fixes, and they look the same if only the read is measured.</para>
///
/// <para>The remainder is reported, not left out. Time that no part accounts for is
/// <see cref="ElsewhereMs"/> and is printed like any other part. If the remainder were dropped, the
/// gap would look fully explained when it is not.</para>
/// </summary>
public readonly record struct FeedGap
{
    /// <param name="totalMs">The whole gap, measured from the pump's side. The sink's own "away"
    /// figure is the same span measured from the sink's side.</param>
    /// <param name="readMs">Taking the block out of the decode-ahead ring.</param>
    /// <param name="changeoverMs">The gapless move onto the record that was queued next.</param>
    /// <param name="playheadMs">Asking the device for its position. This is a call into the sound
    /// library and takes the library's lock.</param>
    /// <param name="collectorMs">Time the runtime spent in garbage collection pauses inside the
    /// gap.</param>
    /// <param name="ringMs">How much audio was waiting in the ring when the block was asked for, or
    /// null where the source has no ring to ask. It is not a part of the gap. It shows whether a slow
    /// read was caused by the decoder or by the pump.</param>
    public FeedGap(
        double totalMs,
        double readMs,
        double changeoverMs,
        double playheadMs,
        double collectorMs,
        double? ringMs)
    {
        // Clamped to zero: these values are differences between timestamps taken on a thread that
        // can be suspended between any two of them. One negative part would make the remainder
        // wrongly large.
        TotalMs = Math.Max(0, totalMs);
        ReadMs = Math.Max(0, readMs);
        ChangeoverMs = Math.Max(0, changeoverMs);
        PlayheadMs = Math.Max(0, playheadMs);
        CollectorMs = Math.Max(0, collectorMs);
        RingMs = ringMs is { } r ? Math.Max(0, r) : null;
    }

    /// <summary>The whole gap.</summary>
    public double TotalMs { get; }

    /// <summary>Time spent taking the block out of the ring. Long means the decoder is behind.</summary>
    public double ReadMs { get; }

    /// <summary>Time spent moving onto the queued next record.</summary>
    public double ChangeoverMs { get; }

    /// <summary>Time spent asking the device for its position.</summary>
    public double PlayheadMs { get; }

    /// <summary>Time the runtime spent in garbage collection inside the gap. It overlaps the parts
    /// above instead of adding to them (a collection pauses whichever part was running), so it is
    /// reported next to them and never subtracted.</summary>
    public double CollectorMs { get; }

    /// <summary>How much audio was waiting to be read, where that can be asked.</summary>
    public double? RingMs { get; }

    /// <summary>Everything the pump did that no part above measured, and the time it was not running.
    /// Never negative, so overlapping parts cannot make it smaller than zero.</summary>
    public double ElsewhereMs => Math.Max(0, TotalMs - ReadMs - ChangeoverMs - PlayheadMs);

    /// <summary>Whether this gap is worth a log line. The device holds half a second of audio, so a gap
    /// close to that is audible.</summary>
    public bool IsLate(double thresholdMs) => TotalMs > thresholdMs;

    /// <summary>The log line, without the tag in front of it.</summary>
    public string Describe() =>
        $"{TotalMs:F0}ms away — read {ReadMs:F0}ms, changeover {ChangeoverMs:F0}ms, " +
        $"playhead {PlayheadMs:F0}ms, elsewhere {ElsewhereMs:F0}ms " +
        $"({(RingMs is { } r ? $"{r:F0}ms was in the ring" : "no ring to ask")}, " +
        $"{CollectorMs:F0}ms of it in the collector)";
}
