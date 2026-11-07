using RPM.Core.Library;

namespace RPM.Core.Cloud;

/// <summary>What one sync pass does with one record's levelling and series: take the store's answer, send
/// this machine's, record that the two already agree, or leave it.
///
/// <para>The rules are those of <see cref="AlbumClassicalPlan"/>, for three values that travel together.
/// The store's answer exists only when a machine has set the settings (<see cref="CloudAlbumMark.SettingsAt"/>):
/// on a row without that stamp, a null levelling and a missing series are silence, not "follow the setting"
/// and "stands alone". A record that says something here and has no answer in the store is sent whether or
/// not this machine changed it, so settings made before they were synced still reach the store.</para>
/// </summary>
public static class AlbumSettingsPlan
{
    /// <summary>Whether the store's answer replaces this machine's: the store has one, it changed since this
    /// machine last accepted one, it differs, and nothing here is waiting to be sent.</summary>
    public static bool Takes(AlbumSettingsSync here, CloudAlbumMark stored)
    {
        ArgumentNullException.ThrowIfNull(here);
        ArgumentNullException.ThrowIfNull(stored);

        return stored.SettingsAt > 0
            && !here.ChangedHere
            && stored.UpdatedAt > here.CloudUpdatedAt
            && !Same(here, stored);
    }

    /// <summary>What the push does with one record.</summary>
    /// <param name="stored">The store's row for this record's key, or null when there is none. Stated rather
    /// than defaulted, because "no row" is an answer that the rule depends on.</param>
    public static AlbumClassicalPlan.Outbound Out(AlbumSettingsSync here, CloudAlbumMark? stored)
    {
        ArgumentNullException.ThrowIfNull(here);

        var answered = stored is { SettingsAt: > 0 };

        if (here.ChangedHere)
            // No answer in the store reads as "follow the setting" and "stands alone" everywhere, so the
            // same here is agreement.
            return (answered ? Same(here, stored!) : !here.SaysAnything)
                ? AlbumClassicalPlan.Outbound.Agreed
                : AlbumClassicalPlan.Outbound.Send;

        return here.SaysAnything && !answered ? AlbumClassicalPlan.Outbound.Send : AlbumClassicalPlan.Outbound.Leave;
    }

    /// <summary>Whether the store's three settings are the same as this machine's.</summary>
    public static bool Same(AlbumSettingsSync here, CloudAlbumMark stored)
    {
        ArgumentNullException.ThrowIfNull(here);
        ArgumentNullException.ThrowIfNull(stored);

        return here.Normalize == stored.Normalize && Equals(here.Series, stored.InSeries);
    }
}
