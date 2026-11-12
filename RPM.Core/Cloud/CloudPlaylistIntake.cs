namespace RPM.Core.Cloud;

/// <summary>What a machine should do with one playlist it has found in the cloud.</summary>
public enum PlaylistIntake
{
    /// <summary>Make it here: nothing on this machine claims it, it was not let go, and every song
    /// it names is a song that this library has.</summary>
    Take,

    /// <summary>A node here already claims this cloud id. The local node is the running order from
    /// then on, and the sync run's push keeps the cloud copy in step with it.</summary>
    AlreadyHere,

    /// <summary>This library deleted it, or took it out of the cloud, and the copy up there has not
    /// gone yet. It is never taken in: that is the purpose of the note.</summary>
    LetGo,

    /// <summary>It names songs that this machine does not have, so making it here would make a shorter
    /// playlist than the one that was written. It is left where it is and checked again on the next
    /// run.</summary>
    Incomplete,
}

/// <summary>One cloud playlist and what is to become of it here.</summary>
/// <param name="Missing">How many of its songs this library does not have. Zero for every verdict
/// except <see cref="PlaylistIntake.Incomplete"/>, where it is the reason.</param>
public sealed record PlaylistIntakeReading(CloudPlaylist Playlist, PlaylistIntake Verdict, int Missing);

/// <summary>
/// Which playlists in the cloud a computer should take into its own tree.
///
/// <para>
/// A phone can make a playlist, so a cloud playlist that no node here claims might be a leftover
/// from this library, or might be something that the user wrote on the train. The playlist itself
/// cannot tell the two apart. The note that this library keeps of what it let go of can, and that
/// note is a stored fact (<c>pl_cloud_gone</c>) and not a time window: the answer has to be the same
/// whether the sync runs a second after the delete or a fortnight later on a machine that was
/// switched off.
/// </para>
///
/// <para>
/// Taking one in has a consequence, which is what <see cref="PlaylistIntake.Incomplete"/> is about.
/// A playlist made here is pushed back up from here, and the push writes the order that it can
/// actually play. Taking in a list that names a song this machine has never had would quietly
/// publish a shorter list to every other head that the user owns. A playlist is taken in whole or
/// not at all, and the songs it is waiting for usually arrive in the same sync run's own pull.
/// </para>
/// </summary>
public static class CloudPlaylistIntake
{
    /// <summary>
    /// Reads one listing of cloud playlists against what this library holds.
    /// </summary>
    /// <param name="theirs">Every playlist the account keeps in the cloud.</param>
    /// <param name="claimedHere">The cloud ids that nodes in this tree already stand for. It is passed
    /// explicitly and has no default: an empty set means "this library claims nothing", which is right
    /// for a fresh install and wrong everywhere else.</param>
    /// <param name="letGo">The cloud ids this library has deleted or taken out of the cloud:
    /// <see cref="Library.LibraryDatabase.CloudPlaylistsLetGo"/>.</param>
    /// <param name="hasTrack">Whether this library holds the song with that rpm id.</param>
    public static IReadOnlyList<PlaylistIntakeReading> Read(
        IReadOnlyList<CloudPlaylist> theirs,
        IReadOnlySet<string> claimedHere,
        IReadOnlySet<string> letGo,
        Func<string, bool> hasTrack)
    {
        ArgumentNullException.ThrowIfNull(theirs);
        ArgumentNullException.ThrowIfNull(claimedHere);
        ArgumentNullException.ThrowIfNull(letGo);
        ArgumentNullException.ThrowIfNull(hasTrack);

        var readings = new List<PlaylistIntakeReading>(theirs.Count);

        foreach (var playlist in theirs)
        {
            if (claimedHere.Contains(playlist.PlaylistId))
            {
                readings.Add(new PlaylistIntakeReading(playlist, PlaylistIntake.AlreadyHere, Missing: 0));
                continue;
            }

            // Before the songs are counted, not after: a playlist that this library let go is not taken in
            // whatever it holds, and checking its songs first would only be wasted work.
            if (letGo.Contains(playlist.PlaylistId))
            {
                readings.Add(new PlaylistIntakeReading(playlist, PlaylistIntake.LetGo, Missing: 0));
                continue;
            }

            // Counted and not stopped at the first, because the count is what the log line says, and
            // "waiting for forty songs" and "waiting for one" are different situations. A song named twice
            // counts twice: a list that plays it twice is short by two entries.
            var missing = playlist.TrackIds.Count(id => !hasTrack(id));

            readings.Add(new PlaylistIntakeReading(
                playlist, missing > 0 ? PlaylistIntake.Incomplete : PlaylistIntake.Take, missing));
        }

        return readings;
    }

    /// <summary>How far the store's clock and this machine's may disagree. A playlist published from
    /// here less than this long before the listing was asked for is not judged by that listing.</summary>
    public static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The playlists this machine keeps in the cloud that another device has deleted there, by node id.
    ///
    /// <para>A playlist counts only when all of this holds. This machine published it, so the store gave it a
    /// stamp. That stamp is older than the moment the listing was asked for, less
    /// <see cref="ClockSlack"/>: a playlist published while the listing was on its way is not in it yet, and is
    /// not gone. It is still synced from here: one already let go has nothing left to decide. And the
    /// listing does not name it.</para>
    ///
    /// <para>An empty listing decides nothing. It is the answer an account with no playlists gets, and also
    /// the answer a fault on the store's side would give, and the two cannot be told apart. Letting every
    /// playlist go on a fault would stop them all syncing, so an account whose last playlist was deleted
    /// elsewhere keeps it marked here until the listing holds something again.</para>
    ///
    /// <para>The caller lets these go (<see cref="Library.LibraryDatabase.SetPlaylistOutOfCloud"/>). The
    /// playlist stays on this machine. It is not deleted: the device that deleted it deleted the cloud's
    /// copy, and this machine's copy is the user's own.</para>
    /// </summary>
    /// <param name="theirs">The whole listing, as read.</param>
    /// <param name="published">What this machine has published, by cloud id:
    /// <see cref="Library.LibraryDatabase.PlaylistCloudStamps"/>.</param>
    /// <param name="synced">The nodes still synced to the cloud from here.</param>
    /// <param name="listedAt">When the listing was asked for, in Unix milliseconds by this machine's clock.</param>
    public static IReadOnlyList<long> GoneElsewhere(
        IReadOnlyList<CloudPlaylist> theirs,
        IReadOnlyDictionary<string, Library.PlaylistCloudState> published,
        IReadOnlySet<long> synced,
        long listedAt)
    {
        ArgumentNullException.ThrowIfNull(theirs);
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(synced);

        if (theirs.Count == 0) return [];

        var listed = theirs.Select(p => p.PlaylistId).ToHashSet(StringComparer.Ordinal);
        var before = listedAt - (long)ClockSlack.TotalMilliseconds;

        return published
            .Where(entry => entry.Value.Stamp > 0
                         && entry.Value.Stamp < before
                         && synced.Contains(entry.Value.NodeId)
                         && !listed.Contains(entry.Key))
            .Select(entry => entry.Value.NodeId)
            .ToList();
    }

    /// <summary>
    /// The name this machine should take from a cloud copy that moved on, or null to keep its own.
    ///
    /// <para>It is the cloud's name when the cloud's name and folder are no longer what this machine last
    /// agreed with, so another device renamed it, and this machine's own are still what it agreed with, so
    /// nobody renamed it here since. A rename on both sides keeps this machine's name, and the push sends
    /// it. Only the name is taken. A different folder is a move made on another computer; a phone does not
    /// move playlists, and the folders of two computers are their own.</para>
    /// </summary>
    /// <param name="agreed">What this machine last published or agreed with:
    /// <see cref="Library.PlaylistCloudState.SentMeta"/>. Null when nothing was recorded, and then nothing
    /// is taken, because there is no way to tell who renamed it.</param>
    public static string? NameToTake(
        string? agreed, CloudPlaylist theirs, string localName, IReadOnlyList<string> localFolder)
    {
        ArgumentNullException.ThrowIfNull(theirs);
        ArgumentNullException.ThrowIfNull(localFolder);

        if (agreed is null) return null;

        var theirMeta = Library.PlaylistCloudState.MetaOf(theirs.Name, theirs.FolderChain);
        var localMeta = Library.PlaylistCloudState.MetaOf(localName, localFolder);

        if (string.Equals(theirMeta, agreed, StringComparison.Ordinal)) return null;
        if (!string.Equals(localMeta, agreed, StringComparison.Ordinal)) return null;
        if (string.IsNullOrWhiteSpace(theirs.Name)) return null;

        return string.Equals(theirs.Name, localName, StringComparison.Ordinal) ? null : theirs.Name;
    }
}
