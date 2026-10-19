using System.Text.Json.Serialization;

namespace RPM.Core.Cloud;

/// <summary>How much of the library the cloud is meant to hold.</summary>
/// <remarks>This type is here and not beside the settings file where it is stored, because its
/// meaning belongs to the sync engine. See <see cref="CloudSyncReaction"/>, which is all that
/// changing it does.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CloudSyncMode>))]
public enum CloudSyncMode
{
    /// <summary>Nothing goes up. This is the default: the cloud is opt-in, and the app is complete
    /// without it.</summary>
    None = 0,

    /// <summary>Only the tracks the user picked.</summary>
    Selected = 1,

    /// <summary>The whole library, and anything added to it from now on.</summary>
    All = 2,
}

/// <summary>What a settings change asks the sync engine to do. Any combination, including none.</summary>
[Flags]
public enum CloudReaction
{
    None = 0,

    /// <summary>Move the queue to the new mode: stop the worker, then adopt or drop what the previous
    /// mode had queued.</summary>
    ApplyMode = 2,

    /// <summary>Nudge the queue. Only ever on its own: applying a mode kicks the queue itself once the
    /// rows are right, and kicking before that would drain the old mode's queue into the new one.</summary>
    KickQueue = 4,
}

/// <summary>
/// What the sync engine does when the settings change.
///
/// <para>
/// Every setting in the app arrives through one event, so this is asked on every keystroke of a
/// rename and every drag of the cover-size slider. It must therefore not kick the queue for an
/// unrelated setting: in "all music" mode that would upload the entire collection because of a
/// slider.
/// </para>
/// </summary>
public static class CloudSyncReaction
{
    public static CloudReaction For(CloudSyncMode was, CloudSyncMode now)
    {
        // A mode change owns the queue from here: it stops the worker, rewrites the rows, and kicks when
        // it is done. Adding KickQueue would be a second run against rows that are being rewritten.
        if (now != was)
            return CloudReaction.ApplyMode;

        return now == CloudSyncMode.None ? CloudReaction.None : CloudReaction.KickQueue;
    }
}
