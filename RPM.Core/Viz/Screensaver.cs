namespace RPM.Core.Viz;

/// <summary>
/// Whether a full-screen player's screens are put away so that only the room shows. When nothing has been
/// pressed or moved for the minutes the settings name (<see cref="Choices"/>), the screens fade out and the
/// room is left. The next key brings them back as they were, and does nothing else.
///
/// <para>The idle time is worked out from the time of the last key, not from a timer that a key cancels. The
/// host's timer wakes when the time is up, asks <see cref="DueIn"/> again, and only then puts the screens away
/// (<see cref="PutAway"/>). A key that arrives while the timer runs out has moved the time on before it is
/// read, whichever of the two the thread handles first.</para>
///
/// <para>The press that brings the screens back is swallowed down to its release: its repeats while it is
/// held, and the release itself, which some keys act on. The host decides which keys still do their own work
/// through it (the media keys). The times are milliseconds on any clock that only goes forward, passed in, so
/// this holds no clock of its own.</para>
///
/// <para>The Android TV app has the same rule, the same choices and the same default.</para>
/// </summary>
public sealed class Screensaver
{
    /// <summary>The choices in minutes, in the order the settings row steps through them. 0 is off.</summary>
    public static readonly IReadOnlyList<int> Choices = [0, 1, 2, 5, 10, 15, 30];

    /// <summary>The choice until the listener makes one.</summary>
    public const int DefaultMinutes = 2;

    /// <summary>The stored <paramref name="minutes"/> as a choice: a figure that is not one of
    /// <see cref="Choices"/> is the default.</summary>
    public static int Minutes(int minutes) => Choices.Contains(minutes) ? minutes : DefaultMinutes;

    /// <summary>The choice after <paramref name="minutes"/>, wrapping from the longest to off.</summary>
    public static int Next(int minutes)
    {
        var at = -1;
        for (var i = 0; i < Choices.Count; i++)
            if (Choices[i] == Minutes(minutes))
                at = i;
        return Choices[(at + 1) % Choices.Count];
    }

    private long _lastActivityAt;
    private int? _wakingKey;

    /// <summary>Starts with the screens up, counting from <paramref name="now"/>.</summary>
    public Screensaver(long now) => _lastActivityAt = now;

    /// <summary>True while the screens are put away (fading out or gone).</summary>
    public bool Shown { get; private set; }

    /// <summary>
    /// A key went down (a repeat too). Returns true when the press goes no further: it brought the screens
    /// back, or the key that did is still held (its repeats). The rule asks for the key's release rather than
    /// for a repeat flag, which not every platform sets.
    /// </summary>
    public bool KeyDown(int key, long now)
    {
        _lastActivityAt = now;
        if (Shown)
        {
            Shown = false;
            _wakingKey = key;
            return true;
        }
        return _wakingKey == key;
    }

    /// <summary>A key came up. Returns true when it is the release of the press that brought the screens
    /// back, which goes no further either.</summary>
    public bool KeyUp(int key)
    {
        if (_wakingKey != key)
            return false;
        _wakingKey = null;
        return true;
    }

    /// <summary>Something other than a key was used, such as the mouse. Returns true when this brought the
    /// screens back.</summary>
    public bool Used(long now)
    {
        _lastActivityAt = now;
        if (!Shown)
            return false;
        Shown = false;
        return true;
    }

    /// <summary>How long until the screens go, in milliseconds, with <paramref name="minutes"/> chosen. Zero
    /// or less means now. Null while the choice is off.</summary>
    public long? DueIn(int minutes, long now) =>
        minutes <= 0 ? null : _lastActivityAt + (minutes * 60_000L) - now;

    /// <summary>The time is up: the screens are put away.</summary>
    public void PutAway() => Shown = true;

    /// <summary>The screens come back without a key, and the count starts again from
    /// <paramref name="now"/>. For the player going away, or a screen coming up that the screensaver does
    /// not cover.</summary>
    public void Reset(long now)
    {
        _lastActivityAt = now;
        Shown = false;
        _wakingKey = null;
    }
}
