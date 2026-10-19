namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Where the sound is, from a sink that can only say where it last was.
///
/// <para>
/// An audio sink counts whole buffers as the device hands them back. So the position it reports is a
/// staircase: read every frame, it stays still for six frames at a time and then jumps one step. A seek bar
/// cannot show that. A line of text sliding past can, and it is the only thing in the app fine enough to
/// notice. That is why this class exists.
/// </para>
///
/// <para>
/// <para>
/// There are two corrections, and both belong to the output path. Between reports the clock keeps running,
/// so the playhead moves every frame instead of six times a second. The reading is also moved forward by a
/// fixed lead, because what has been handed back is not what is being heard. The device is already playing
/// the next buffer, and the queue adds its own render latency on top. Without this lead the words land
/// after the singer, which is the one direction a lyric must never be wrong.
/// </para>
/// </para>
///
/// <para>This is kept apart from the screen that reads it, for the reason <see cref="LyricHush"/> gives: a
/// rule with no window around it can be tested.</para>
/// </summary>
internal sealed class OutputPlayhead
{
    /// <summary>How coarsely the sink reports: iOS's <c>AudioQueueSink</c> counts whole 50 ms buffers. The
    /// clock is never run further than this past a reading, or a stall would read as the song still
    /// running.</summary>
    internal const double Step = 0.05;

    /// <summary>How far behind the sound the sink's own count runs: the buffer already playing when the
    /// last one came back, plus the queue's render latency. This is one stated figure, not a measurement,
    /// because AudioQueue does not report the second half.</summary>
    internal const double Lead = 0.09;

    /// <summary>Readings this close together are the same reading. A sink that repeats itself has not
    /// moved, so the clock carries on from where that reading landed.</summary>
    private const double Same = 0.001;

    /// <summary>The last reading believed, and when it arrived. NaN until the first one. This is a state,
    /// not a value. It is checked by name below, because no comparison is true against NaN, so a sentinel
    /// left to the comparison would never be replaced.</summary>
    private double _said = double.NaN, _saidAt;

    /// <param name="said">What the sink says its position is, in seconds.</param>
    /// <param name="now">A clock
    /// of this screen's own, in seconds. Only differences against the moment a reading landed are read off
    /// it, so where it starts does not matter.</param>
    internal double At(double said, double now)
    {
        // A new reading, a seek, or a track change: believe it, and count on from there.
        if (double.IsNaN(_said) || Math.Abs(said - _said) > Same)
        {
            _said = said;
            _saidAt = now;
        }

        return _said + Math.Clamp(now - _saidAt, 0, Step) + Lead;
    }

    /// <summary>Forgets the last reading, so the next one is believed outright. Call this when the clock
    /// underneath has been restarted. The moments it held were taken on that clock and mean nothing once it
    /// has restarted.</summary>
    internal void Restart() => _said = double.NaN;
}
