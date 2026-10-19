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
}
