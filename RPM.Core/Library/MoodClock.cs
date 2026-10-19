namespace RPM.Core.Library;

/// <summary>
/// Which mood the hour asks for.
///
/// <para><b>What it is for.</b> The home banner offers one generated list among its five cards. Which
/// list it offers depends on the time of day. A shelf has mood lists on it, and the clock decides which
/// one is the right one to show. Without this, the banner would offer the same list at every hour, and
/// it would never offer something to fall asleep to at one in the morning.</para>
///
/// <para><b>Every band names all five.</b> A collection only has the mood lists that its measured music
/// can fill. A shelf with no measured pass has none of them, and a shelf with little quiet music has no
/// Sleep list. A band that named only one mood would be silent on most shelves. The order is the band's
/// preference, and the caller takes the first mood that is actually offered.</para>
///
/// <para>The hour is local time, and it is read when the screen is built. A home screen that stays open
/// across a band's edge keeps the card it opened with. This decides what to offer, not what to
/// interrupt.</para>
/// </summary>
public static class MoodClock
{
    /// <summary>The moods to offer at <paramref name="hour"/>, best first.
    /// </summary>
    /// <param name="hour">The hour of a local day, 0 to 23. Any other value is wrapped into that range, so
    /// a caller doing arithmetic on the clock cannot pass an hour that is refused.</param>
    public static IReadOnlyList<SmartPlaylistKind> Order(int hour) => (((hour % 24) + 24) % 24) switch
    {
        // Night, and on through the small hours. Sleep comes first. It is the one mood that asks an
        // absolute question of the whole collection rather than a question per genre, which makes it the
        // one worth showing at midnight. The band starts at ten at night, because that is when somebody
        // using this screen for music is winding down rather than starting something.
        >= 22 or <= 4 => [SmartPlaylistKind.Sleep, SmartPlaylistKind.Calm, SmartPlaylistKind.Focus,
                          SmartPlaylistKind.Danceable, SmartPlaylistKind.Energetic],

        // Early morning. Calm music comes first.
        >= 5 and <= 9 => [SmartPlaylistKind.Calm, SmartPlaylistKind.Focus, SmartPlaylistKind.Sleep,
                          SmartPlaylistKind.Energetic, SmartPlaylistKind.Danceable],

        // The working day. Focus is the steady end of the evenness axis, which is what it is for.
        >= 10 and <= 16 => [SmartPlaylistKind.Focus, SmartPlaylistKind.Calm, SmartPlaylistKind.Energetic,
                            SmartPlaylistKind.Danceable, SmartPlaylistKind.Sleep],

        // Evening.
        >= 17 and <= 19 => [SmartPlaylistKind.Energetic, SmartPlaylistKind.Danceable, SmartPlaylistKind.Focus,
                            SmartPlaylistKind.Calm, SmartPlaylistKind.Sleep],

        // The dancefloor hours, between the evening and the night.
        _ => [SmartPlaylistKind.Danceable, SmartPlaylistKind.Energetic, SmartPlaylistKind.Focus,
              SmartPlaylistKind.Calm, SmartPlaylistKind.Sleep],
    };

    /// <summary>The mood this hour asks for, before anything is known about what the shelf can fill. This
    /// is the head of <see cref="Order"/>, for readers who want the answer without the fallbacks.</summary>
    public static SmartPlaylistKind Best(int hour) => Order(hour)[0];
}
