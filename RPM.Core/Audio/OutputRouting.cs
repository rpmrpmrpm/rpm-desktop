namespace RPM.Core.Audio;

/// <summary>Which speakers the sound belongs on, and whether it is worth moving it there.
///
/// <para>The rule is kept apart from the device handling that carries it out. A sink that only asked where
/// the sound belonged when something had announced a change, or when its own endpoint had gone, would never
/// notice the listener changing the machine's output while the old speakers were still connected. Nothing
/// is disconnected and nothing is added. On a platform that does not announce a default change, or with a
/// sound library too old to pass one on, nothing says so at all. The music would keep playing out of the
/// speakers the listener had just told the machine to stop using.</para>
/// </summary>
public static class OutputRouting
{
    /// <summary>The endpoint to play to: the one the listener chose, when the machine still has it, and the
    /// machine's own default the rest of the time.
    ///
    /// <para>A chosen name that the machine does not have now is not an error, and it does not clear the
    /// choice. An unplugged USB interface is still the listener's choice. So the sound uses the default for
    /// now, and goes back to the chosen endpoint as soon as it is listed again.</para></summary>
    /// <returns>The endpoint's name, or empty for "whatever the machine calls its default". The sound
    /// library is given an empty name when it is asked to open without one.</returns>
    public static string Choose(string? preferred, IReadOnlyCollection<string>? available,
                                string? systemDefault)
    {
        if (!string.IsNullOrEmpty(preferred) && available is not null && available.Contains(preferred))
            return preferred;

        return systemDefault ?? "";
    }

    /// <summary>The endpoint to play to, taken from the sound library's own list of the machine's
    /// endpoints.
    ///
    /// <para>The machine's default output is the FIRST name in that list. The library's separate "what is
    /// the default" call re-reads the machine only when its cached list is empty. Otherwise it answers from
    /// the list it read last, so on its own it gives the same endpoint for the life of the process. Reading
    /// the default from the front of a list that is re-read on every call is current, and it takes one call
    /// instead of two.</para></summary>
    public static string ChooseFromEnumeration(string? preferred, IReadOnlyList<string>? enumeration)
    {
        var machine = enumeration ?? [];
        return Choose(preferred, machine, machine.Count > 0 ? machine[0] : "");
    }

    /// <summary>Whether a device that has returned no data should be presumed dead.
    ///
    /// <para><paramref name="watchedMs"/> is the time the pump itself spent waiting and watching, not the
    /// elapsed wall-clock time. This is the whole rule. Every garbage collection in the process pauses the
    /// managed threads, and a busy UI can pause them for most of a second. For example, the grid rebuilding
    /// can cause 868 ms of pauses in one second. Measured on the clock, that silence looks the same as a
    /// speaker being unplugged. The app would then declare a healthy device dead, hold the track, and stop
    /// the music about ten seconds later. It would blame the speakers for its own pauses.</para>
    ///
    /// <para>Time that the pump did not spend watching cannot be counted. So a suspended process reports a
    /// stall of zero, which is correct: nobody was watching.</para>
    /// </summary>
    /// <param name="watchedMs">How long the pump has waited and seen nothing come back, counted only in the
    /// waits it actually performed.</param>
    /// <param name="noProgressMs">How long a healthy device may go without returning a buffer.</param>
    /// <param name="playing">False while paused or before the source has started. A device owes nothing
    /// then, and silence means nothing.</param>
    public static bool ShouldPresumeLost(int watchedMs, int noProgressMs, bool playing)
        => playing && watchedMs > noProgressMs;

    /// <summary>Whether the sound should be moved.
    ///
    /// <para><paramref name="want"/> and <paramref name="intended"/> are both endpoints we asked for. They
    /// are never what an open device reports it opened. The two strings are not the same. The enumeration
    /// names an endpoint one way, and an opened device names itself another way, with a prefix on Windows.
    /// Comparing across them never matches. The result would be a move at every check, a source stopped
    /// every time, and no sound ever going anywhere new.</para></summary>
    /// <param name="want">The endpoint <see cref="Choose"/> picks now.</param>
    /// <param name="intended">The endpoint asked for last time.</param>
    /// <param name="connected">Whether the sound library still reports the open endpoint as
    /// present.</param>
    /// <param name="lost">Whether the sound is going nowhere, by the pump's own evidence. This outranks
    /// <paramref name="connected"/>, because a driver can keep reporting a dead speaker as present for
    /// several seconds.</param>
    public static bool ShouldMove(string? want, string? intended, bool connected, bool lost)
    {
        // Nowhere to move to. A machine with no playback endpoints at all is a reason to wait, not to move.
        if (string.IsNullOrEmpty(want)) return false;

        // The endpoint we hold is missing, or it is not playing anything, whatever it claims. Asking for
        // the same name again re-opens the device on whatever now answers to that name.
        if (!connected || lost) return true;

        // The endpoint we chose is now different from the one we would choose, even though nothing was
        // disconnected.
        return !string.Equals(want, intended, StringComparison.Ordinal);
    }
}
