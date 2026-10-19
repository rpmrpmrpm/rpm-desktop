using RPM.Core.Library;

namespace RPM.Core.Cloud;

/// <summary>What one sync pass does with one record's classical mark: take the store's answer, send this
/// machine's, record that the two already agree, or leave it.
///
/// <para>The rules are the same as for the favourite (see CloudSyncService.AlbumMarks), with one difference
/// on the way out. A record that is classical here, and has no answer in the store, is sent whether or not
/// this machine changed it. Marks made before the mark was synced have no edit time. A record whose album
/// key changed has no row under its new key. Without this rule, both would never be sent.</para>
/// </summary>
public static class AlbumClassicalPlan
{
    /// <summary>What the push does with one record.</summary>
    public enum Outbound
    {
        /// <summary>Nothing to say.</summary>
        Leave,

        /// <summary>Changed here, and the store already holds the same answer. Record the agreement without
        /// a write.</summary>
        Agreed,

        /// <summary>Write this machine's answer.</summary>
        Send,
    }

    /// <summary>Whether the store's answer replaces this machine's.
    ///
    /// <para>This applies only when the store has an answer at all (null means nobody has said anything).
    /// The answer must have changed since this machine last accepted one. It must differ from what is here,
    /// and nothing here may be waiting to be sent. The last condition makes an edit made here win over an
    /// older edit from elsewhere.</para>
    /// </summary>
    public static bool Takes(AlbumClassicalSync here, CloudAlbumMark stored)
    {
        ArgumentNullException.ThrowIfNull(here);
        ArgumentNullException.ThrowIfNull(stored);

        return stored.Classical is { } said
            && !here.ChangedHere
            && stored.UpdatedAt > here.CloudUpdatedAt
            && said != here.IsClassical;
    }

    /// <summary>What the push does with one record.
    /// </summary>
    /// <param name="stored">The store's row for this record's key, or null when there is none. The null is
    /// stated rather than defaulted, because "no row" is an answer that the rule depends on.</param>
    public static Outbound Out(AlbumClassicalSync here, CloudAlbumMark? stored)
    {
        ArgumentNullException.ThrowIfNull(here);

        var answer = stored?.Classical;

        if (here.ChangedHere)
            // An absent answer reads as "not classical" everywhere, so "not classical" against no answer is
            // agreement.
            return (answer ?? false) == here.IsClassical ? Outbound.Agreed : Outbound.Send;

        return here.IsClassical && answer is null ? Outbound.Send : Outbound.Leave;
    }
}
