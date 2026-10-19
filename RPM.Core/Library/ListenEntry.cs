using RPM.Core.Playback;

namespace RPM.Core.Library;

/// <summary>
/// One finished listen, on its way into the log.
///
/// <para>There is no public constructor, and that is the whole point of the type. A listen has to
/// carry the hour and the weekday it happened on <em>in the listener's own clock</em>, and those two
/// cannot be worked out afterwards: the offset that turns a UTC tick into "half past eleven at night"
/// is the offset that was in force at that moment, on the machine it happened on. The user gets on a
/// plane, the clocks go back, and every stored tick quietly means a different hour than it did. So the
/// answer is stamped while it is still true, and the only way to build one of these is
/// <see cref="At"/>, which asks for the reading rather than the instant.</para>
///
/// <para>Everything else here IS derivable and so is not stored: the running total lives on the track
/// row, the RPM id is looked up by the write, and whether the listen counted is
/// <see cref="Listen.Counts"/> asked once and recorded so a later change to the rule cannot rewrite
/// what somebody did.</para>
/// </summary>
public sealed record ListenEntry
{
    private ListenEntry(string relativePath, DateTimeOffset when, double heardSeconds, double lengthSeconds)
    {
        RelativePath = relativePath;
        AtUtcTicks = when.UtcTicks;
        LocalHour = when.Hour;
        LocalDayOfWeek = (int)when.DayOfWeek;
        HeardSeconds = heardSeconds;
        LengthSeconds = lengthSeconds;
        Counted = Listen.Counts(heardSeconds, lengthSeconds);
    }

    /// <summary>The track as the mirror knows it right now. The log is re-pointed with the row when a file
    /// is re-filed (see <c>MoveLocked</c>), so this stays the join.</summary>
    public string RelativePath { get; }

    /// <summary>When the listen ENDED, in UTC. Ended rather than started, because that is the moment the
    /// app knows how much was heard.</summary>
    public long AtUtcTicks { get; }

    /// <summary>Audio actually delivered, not the position reached.</summary>
    public double HeardSeconds { get; }

    public double LengthSeconds { get; }

    /// <summary>What <see cref="Listen.Counts"/> said at the time: a play, or a skip.</summary>
    public bool Counted { get; }

    /// <summary>0 to 23 on the listener's own clock. See the note on the type.</summary>
    public int LocalHour { get; }

    /// <summary>0 = Sunday, matching <see cref="DayOfWeek"/>.</summary>
    public int LocalDayOfWeek { get; }

    /// <summary>A listen that has just ended, read off a local clock.
    /// </summary>
    /// <param name="when">The local reading: <c>DateTimeOffset.Now</c> in the app, a fixed value in a test.
    /// Not <c>DateTime.UtcNow</c>: that has no offset, and the offset is the part that cannot be recovered
    /// later.</param>
    /// <returns>The entry, or null when the listen is too slight to write down. See
    /// <see cref="Listen.WorthRecording"/>. Null rather than a flag, so a caller cannot write one by
    /// forgetting to ask.</returns>
    public static ListenEntry? At(DateTimeOffset when, string relativePath, double heardSeconds, double lengthSeconds)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        return Listen.WorthRecording(heardSeconds, lengthSeconds)
            ? new ListenEntry(relativePath, when, heardSeconds, lengthSeconds)
            : null;
    }
}
