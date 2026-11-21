namespace RPM.Core.Lyrics;

/// <summary>
/// The record's time for the karaoke words: the player's position, made steady. The Android phone app's
/// <c>KaraokeClock</c> has the same rule.
///
/// <para>The position a player reports moves in steps. On the desktop the sound device's count is
/// published once for each 50 ms block of audio. Read on every frame, it stands still for two or three
/// frames and then jumps 40 to 60 ms, while the sound itself plays on evenly. On some phones it also jumps
/// back by up to a quarter of a second. The lit letters follow the time they are given, so a step forward
/// lights several letters at once, and a step back puts out letters that are already lit.</para>
///
/// <para>This clock runs at the speed of the wall clock and moves towards the reported position slowly,
/// never faster than <see cref="Slew"/> of the time that passes. So it moves on every frame and never goes
/// back while the record plays. A difference larger than <see cref="Snap"/> is a seek or a new record, and
/// the clock goes straight there. So does a record that is not playing.</para>
///
/// <para>One clock serves one view of the lyrics, on one thread.</para>
/// </summary>
public sealed class KaraokeClock
{
    /// <summary>How much faster or slower than the wall clock the clock may run while it moves to the
    /// reported position.</summary>
    public const double Slew = 0.05;

    /// <summary>A difference from the reported position, in seconds, past which the clock goes straight
    /// there.</summary>
    public const double Snap = 0.6;

    /// <summary>The record's time last shown, and the moment it was shown at on the caller's clock. NaN
    /// until the first reading.</summary>
    private double _shown = double.NaN, _at;

    /// <summary>The record's time to show, in seconds.</summary>
    /// <param name="reported">Where the player says the record is, in seconds.</param>
    /// <param name="advancing">Whether the record is playing.</param>
    /// <param name="now">A steady clock of the caller's own, in seconds. Only differences between two
    /// readings are used, so where it starts does not matter.</param>
    public double Seconds(double reported, bool advancing, double now)
    {
        if (!advancing || double.IsNaN(_shown))
            return Set(reported, now);

        // Asked about a moment before the last one: read off the clock, which is not moved.
        if (now < _at)
            return _shown - (_at - now);

        var passed = now - _at;
        var carried = _shown + passed;
        var off = reported - carried;
        if (Math.Abs(off) > Snap)
            return Set(reported, now);

        return Set(carried + Math.Clamp(off, -passed * Slew, passed * Slew), now);
    }

    /// <summary>Forgets the time shown, so the next reading is taken as it is.</summary>
    public void Reset() => _shown = double.NaN;

    private double Set(double seconds, double now)
    {
        _shown = seconds;
        _at = now;
        return seconds;
    }
}
