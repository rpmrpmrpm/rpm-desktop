namespace RPM.Core.Library;

/// <summary>
/// Whose clock the generated lists are cut on.
///
/// <para>Two lists depend on the local time: "Your mix" leans towards the music played at this hour, and the
/// holiday lists (Christmas, New Year's Eve) appear on certain dates. On a computer the listener sits at the
/// machine, so its own zone is the right one. The store cuts lists for a phone in another zone, so it says
/// which zone with <see cref="ListenFrom"/> before anything is cut.</para>
///
/// <para>The hours in the listening log do not depend on this. Each listen is stamped with the hour on the
/// clock of the device that played it (<see cref="ListenEntry.LocalHour"/>).</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>Guarded by <see cref="_cuts"/>, because the session cuts read it.</summary>
    private TimeZoneInfo _listenerZone = TimeZoneInfo.Local;

    /// <summary>The time zone of the person the lists are cut for. This machine's zone unless
    /// <see cref="ListenFrom"/> was called.</summary>
    public TimeZoneInfo ListenerZone
    {
        get
        {
            using (_cuts.Take())
                return _listenerZone;
        }
    }

    /// <summary>
    /// Cuts the lists on <paramref name="zone"/> instead of this machine's zone.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session's cuts were already started. They are held
    /// for the session, so a later zone would leave them on one clock and the hour of "Your mix" on
    /// another.</exception>
    public void ListenFrom(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        using (_cuts.Take())
        {
            if (_moods.Started || _profile.Started)
                throw new InvalidOperationException("The session's lists are already cut on another clock.");
            _listenerZone = zone;
        }
    }

    /// <summary>A UTC instant as the listener's own clock shows it.</summary>
    internal DateTime ListenerLocal(long utcTicks) =>
        TimeZoneInfo.ConvertTimeFromUtc(new DateTime(utcTicks, DateTimeKind.Utc), ListenerZone);
}
