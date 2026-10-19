namespace RPM.Core.Cloud;

/// <summary>Which files the player has open. Nothing else may rewrite or move these files.
///
/// <para>Editing a track's tags, splitting a record, or writing an analysis back into a file all rewrite
/// the file in place. Doing that to the track that is playing either fails, because a handle is open on
/// it, or appears to work and then breaks the next seek.
/// </para>
///
/// <para>This rule lives here, not in the player, because it decides what may be written, not how a file
/// is played. The comparison is where mistakes happen: a null, a path that differs only in case, or the
/// next track that a gapless changeover has already opened.
/// </para>
/// </summary>
public static class OpenFiles
{
    /// <summary>
    /// Whether a player holding <paramref name="playing"/> open, with
    /// <paramref name="queuedNext"/> already opened behind it, is using this file.
    /// </summary>
    /// <param name="path">The file about to be written to.</param>
    /// <param name="playing">What the engine has on, or null.</param>
    /// <param name="queuedNext">The successor the gapless changeover has opened, or null. This is also
    /// null when the successor is a stream, because a stream has no file behind it.</param>
    public static bool HeldOpen(string? path, string? playing, string? queuedNext)
    {
        if (string.IsNullOrEmpty(path)) return false;

        return Same(playing, path) || Same(queuedNext, path);

        // Case-insensitive. The mirror's paths and the ones handed to the engine come from the same
        // place and should match exactly. Still, a difference in case must never mean "a different
        // file, safe to write to", because on the platforms rpm runs on it is the same file.
        static bool Same(string? held, string? path) =>
            held is { Length: > 0 } && string.Equals(held, path, StringComparison.OrdinalIgnoreCase);
    }
}
